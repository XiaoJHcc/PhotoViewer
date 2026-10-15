using AppKit;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Settings;
using PhotoViewer.Core.Platform;
using PhotoViewer.ViewModels.Settings;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoViewer.Core;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Mac.Core;
using System.Runtime.InteropServices;
using PhotoViewer.Core.Database;

namespace PhotoViewer.Mac.Native;

/// <summary>真实原生窗口的程序化冒烟，仅显式 --pv-native-smoke 激活，不更改用户照片。</summary>
public static class NativeAcceptanceRunner
{
    /// <summary>检查首图、代理升级、真实翻页、设置分类与持久化契约，输出可复核报告。</summary>
    public static async Task RunAsync(MacMainWindow window, MacNativeSettingsPresenter settings, string[] args)
    {
        var results = new List<NativeCheck>();
        string output = System.IO.Path.GetFullPath("release/native-p2-acceptance");
        Directory.CreateDirectory(output);
        try
        {
            await WaitAsync(() => window.Main.CurrentFile != null && window.Session.Image != null, "首图加载");
            if (args.Contains("--pv-native-toolbar"))
            {
                await NativeToolbarAcceptance.RunAsync(window, output);
                File.Delete(System.IO.Path.Combine(output, "native-smoke-error.txt"));
                Console.WriteLine("[NativeSmoke] PASS toolbar");
                Environment.ExitCode = 0;
                return;
            }
            if (args.Contains("--pv-native-layout"))
            {
                await NativeMainLayoutAcceptance.RunAsync(window, output);
                File.Delete(System.IO.Path.Combine(output, "native-smoke-error.txt"));
                Console.WriteLine("[NativeSmoke] PASS layout");
                Environment.ExitCode = 0;
                return;
            }
            if (args.Contains("--pv-native-settings"))
            {
                await NativeSettingsAcceptance.RunAsync(window, settings, output);
                File.Delete(System.IO.Path.Combine(output, "native-smoke-error.txt"));
                Console.WriteLine("[NativeSmoke] PASS settings");
                Environment.ExitCode = 0;
                return;
            }
            if (args.Contains("--pv-native-filmstrip"))
            {
                await NativeFilmstripAcceptance.RunAsync(window, output);
                Console.WriteLine("[NativeSmoke] PASS filmstrip");
                Environment.ExitCode = 0;
                return;
            }
            var first = window.Main.CurrentFile!;
            results.Add(new NativeCheck { Check = "startup", File = first.Name, Encoding = window.Session.Image!.Encoding.ToString(), ColorSpace = window.Session.Image.ColorSpace.ToString(), Width = window.Session.Image.Width, Height = window.Session.Image.Height, SourceWidth = window.Session.Image.SourceWidth, SourceHeight = window.Session.Image.SourceHeight, Proxy = window.Session.IsProxy });
            window.Photo.ZoomToPhysical(1);
            await WaitAsync(() => window.Session.Image != null && !window.Session.IsProxy, "物理 100% Full");
            if (Math.Abs(window.Photo.PhysicalScale - 1) > .001) throw new InvalidOperationException("100% 物理倍率错误");
            results.Add(new NativeCheck { Check = "full-upgrade", Width = window.Session.Image!.Width, Height = window.Session.Image.Height, PhysicalScale = window.Photo.PhysicalScale });
            window.Photo.FitToView();
            var files = window.Main.FileVM.ThumbnailList.FilteredFiles.ToArray();
            foreach (var file in files.Take(12))
            {
                var stopwatch = Stopwatch.StartNew();
                window.Main.CurrentFile = file;
                await WaitAsync(() => window.Session.Image != null, "切图 " + file.Name);
                results.Add(new NativeCheck { Check = "flip", File = file.Name, Milliseconds = stopwatch.Elapsed.TotalMilliseconds, Proxy = window.Session.IsProxy });
                if (file.Name.StartsWith("color-", StringComparison.Ordinal))
                {
                    VerifyColor(window.Session.Image!, file.Name);
                    results.Add(new NativeCheck { Check = "imageio-color", File = file.Name });
                }
                if (file.Name.StartsWith("orientation-", StringComparison.Ordinal))
                {
                    VerifyOrientation(window.Photo, window.Session.Image!.Orientation);
                    VerifyAnalysisOrientation(window.Photo, window.Session.Image!);
                    results.Add(new NativeCheck { Check = "gpu-orientation", File = file.Name });
                }
            }
            window.Main.CurrentFile = null;
            ExternalOpenService.PublishFile(new Uri(first.File.PathKey), "NativeSmoke");
            await WaitAsync(() => window.Main.CurrentFile?.File.PathKey == first.File.PathKey && window.Session.Image != null, "外部文件队列打开");
            results.Add(new NativeCheck { Check = "external-open-service", File = first.Name });
            await WaitAsync(() => window.Main.FileVM.ThumbnailList.FilteredFiles.Count == files.Length, "外部打开后文件列表");
            foreach (var mode in new[] { LayoutMode.Row, LayoutMode.Col, LayoutMode.Auto })
            {
                window.Main.Settings.LayoutMode = mode;
                await Task.Delay(100);
                results.Add(new NativeCheck { Check = "layout", Mode = mode.ToString() });
                NativeSnapshots.SaveMain(window, System.IO.Path.Combine(output, "main-" + mode.ToString().ToLowerInvariant() + ".png"));
            }
            window.Main.Settings.LayoutMode = LayoutMode.Col;
            settings.TryPresent(window.Main.Settings);
            for (int category = 0; category < 5; category++)
            {
                settings.ShowCategory(category); await Task.Delay(50);
                if (settings.Window?.ContentView is { } view) NativeSnapshots.SaveView(view, System.IO.Path.Combine(output, "settings-" + category + ".png"));
            }
            int memoryBefore = window.Main.Settings.BitmapCacheMaxMemory;
            bool keyboardBefore = window.Main.Settings.UseAppleKeyboard;
            settings.WorkingSettings.BitmapCacheMaxMemory = memoryBefore + 1;
            settings.WorkingSettings.UseAppleKeyboard = !keyboardBefore;
            if (window.Main.Settings.BitmapCacheMaxMemory != memoryBefore || BitmapLoader.MaxCacheSize != memoryBefore * 1024L * 1024L || PhotoViewer.Core.Input.AppleKeyboardMapping.UseAppleKeyboard != keyboardBefore)
                throw new InvalidOperationException("设置工作副本提前影响运行时配置");
            settings.Window?.Close();
            if (window.Main.Settings.BitmapCacheMaxMemory != memoryBefore + 1 || BitmapLoader.MaxCacheSize != (memoryBefore + 1) * 1024L * 1024L || PhotoViewer.Core.Input.AppleKeyboardMapping.UseAppleKeyboard == keyboardBefore)
                throw new InvalidOperationException("设置关闭回写未应用运行时配置");
            results.Add(new NativeCheck { Check = "settings-working-copy-isolation", JsonRoundTrip = true });
            var snapshot = window.Main.Settings.CreateSnapshot();
            string serialized = JsonSerializer.Serialize(snapshot, SettingsJsonContext.Default.SettingsModel);
            if (JsonSerializer.Deserialize(serialized, SettingsJsonContext.Default.SettingsModel) == null) throw new InvalidOperationException("设置序列化失败");
            await Task.Delay(800);
            string persisted = await File.ReadAllTextAsync(System.IO.Path.Combine(output, "settings-smoke.json"));
            var restored = JsonSerializer.Deserialize(persisted, SettingsJsonContext.Default.SettingsModel) ?? throw new InvalidOperationException("设置落盘为空");
            if (JsonSerializer.Serialize(restored, SettingsJsonContext.Default.SettingsModel) != serialized) throw new InvalidOperationException("设置落盘与关闭回写不一致");
            results.Add(new NativeCheck { Check = "settings", Categories = 5, JsonRoundTrip = true });
            bool generatedFixture = first.File.PathKey.Contains(System.IO.Path.DirectorySeparatorChar + "native-p2-fixtures" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
            bool rawCopy = first.File.PathKey.Contains(System.IO.Path.DirectorySeparatorChar + "native-p2-raw" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (generatedFixture || rawCopy)
            {
                await window.RateAsync(first, 3);
                var exif = await ExifLoader.LoadExifDataAsync(new MacFileRef(first.File.PathKey));
                if (exif?.Rating != 3) throw new InvalidOperationException("原生评分写回读环失败");
                results.Add(new NativeCheck { Check = rawCopy ? "rating-raw-in-place" : "rating-in-place", File = first.Name, JsonRoundTrip = true });
                string sidecarPath = System.IO.Path.Combine(output, "rating-service.xmp");
                await File.WriteAllTextAsync(sidecarPath, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"0\"/></rdf:RDF></x:xmpmeta>");
                if (!await XmpWriter.WriteRatingAsync(new MacFileRef(sidecarPath), 4, true) || !(await File.ReadAllTextAsync(sidecarPath)).Contains("xmp:Rating=\"4\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("独立 XMP 文件原位写回失败");
                results.Add(new NativeCheck { Check = "rating-existing-xmp-service", JsonRoundTrip = true });
                using var analysis = ColorEngine.ToAnalysisSrgb8(window.Session.Image!);
                var cv = await CvGridExtractor.ExtractAsync(analysis);
                if (cv.Encode().Length == 0) throw new InvalidOperationException("CV 输出为空");
                results.Add(new NativeCheck { Check = "cv-native-adapter", Width = analysis.Width, Height = analysis.Height });
                using var thumbnail = await ThumbnailService.GetThumbnailAsync(first.File, 560, DecodePriority.P3_Background) ?? throw new InvalidOperationException("DINO 缩略图无法取得");
                var features = await DinoFeatureExtractor.ExtractDualAsync(thumbnail, includePatches: true);
                if (features.Cls.Length == 0 || features.Patches == null || !features.Cls.All(float.IsFinite)) throw new InvalidOperationException("DINO 双输出推理失败");
                results.Add(new NativeCheck { Check = "dino-native-resources", Width = features.Cls.Length, Height = features.Patches.Length });
                var indexer = new FolderFeatureIndexer();
                await indexer.RunAsync([first]);
                if (indexer.LastError != null) throw new InvalidOperationException("索引写库失败：" + indexer.LastError);
                var groups = await FolderFeatureIndexer.GroupByFingerprintAsync([first]);
                if (groups.Count != 1 || string.IsNullOrEmpty(groups[0].Fingerprint) || (await FolderFeatureIndexer.EvaluateMissingPartsAsync(groups[0].Fingerprint)).AnyMissing)
                    throw new InvalidOperationException("指纹索引三路持久化不完整");
                results.Add(new NativeCheck { Check = "index-fingerprint-persistence", JsonRoundTrip = true });
            }
            PhotoDatabase.Initialize();
            using (var database = PhotoDatabase.OpenConnection())
            using (var query = database.CreateCommand())
            {
                query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('photos','photo_features','photo_patches')";
                if (Convert.ToInt64(query.ExecuteScalar()) != 3) throw new InvalidOperationException("指纹缓存 schema 不完整");
                results.Add(new NativeCheck { Check = "database-schema", JsonRoundTrip = true });
            }
            if (window.Main.ImageVM.SourceBitmap != null) throw new InvalidOperationException("原生壳仍创建 Avalonia 主图");
            results.Add(new NativeCheck { Check = "native-boundary", NoAvaloniaMainBitmap = true, GpuBytes = window.Photo.FootprintBytes });
            string report = JsonSerializer.Serialize(results, NativeSmokeJsonContext.Default.ListNativeCheck);
            await File.WriteAllTextAsync(System.IO.Path.Combine(output, "native-smoke.json"), report);
            string reportName = rawCopy ? "native-smoke-raw.json" : first.Name.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ? "native-smoke-heif.json" : "native-smoke-fixtures.json";
            await File.WriteAllTextAsync(System.IO.Path.Combine(output, reportName), report);
            File.Delete(System.IO.Path.Combine(output, "native-smoke-error.txt"));
            Console.WriteLine("[NativeSmoke] PASS " + results.Count + " checks");
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(output, "native-smoke-error.txt"), exception.ToString());
            Console.Error.WriteLine("[NativeSmoke] FAIL " + exception);
            Environment.ExitCode = 1;
        }
        finally { await Task.Delay(600); Environment.Exit(Environment.ExitCode); }
    }
    /// <summary>用已知测试色块检验 ImageIO 浮点通道和行朝向，P3/Adobe 红原色不能提前裁剪。</summary>
    private static void VerifyColor(DecodedImage image, string filename)
    {
        var values = MemoryMarshal.Cast<byte, Half>(image.Pixels.AsSpan(image.Stride * 10 + 10 * 8, 8));
        float red = (float)values[0], green = (float)values[1], blue = (float)values[2];
        string name = System.IO.Path.GetFileNameWithoutExtension(filename);
        if (name == "color-srgb" && (red < .98f || Math.Abs(green) > .02f || Math.Abs(blue) > .02f)) throw new InvalidOperationException("sRGB 通道或上下方向错误");
        if (name == "color-p3" && (red < 1.15f || green > -.02f)) throw new InvalidOperationException("P3 色域被裁剪或 ICC 丢失");
        if (name is "color-adobe" or "color-untagged-adobe" && red < 1.3f) throw new InvalidOperationException("Adobe RGB 色域被裁剪或 ICC/EXIF 丢失");
    }
    /// <summary>独立以 EXIF 标准角点映射验证 GPU 八种旋转/镜像，采样原色区内部以避开 mipmap 边缘。</summary>
    private static void VerifyOrientation(MetalPhotoView photo, int orientation)
    {
        using var frame = photo.CaptureLinearFrame(navigation: true);
        (double Horizontal, double Vertical)[] coordinates = [(.05, .05), (.95, .05), (.05, .95), (.95, .95)];
        int[][] sourceCorners = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1], [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]];
        double width = frame.Width;
        double height = frame.Height;
        for (int corner = 0; corner < coordinates.Length; corner++)
        {
            int horizontal = (int)(width * coordinates[corner].Horizontal);
            int vertical = (int)(height * coordinates[corner].Vertical);
            var values = MemoryMarshal.Cast<byte, Half>(frame.Pixels.AsSpan(vertical * frame.Stride + horizontal * 8, 8));
            int original = sourceCorners[orientation - 1][corner];
            float red = (float)values[0], green = (float)values[1], blue = (float)values[2];
            bool valid = original switch
            {
                0 => red > .9f && green < .05f && blue < .05f,
                1 => blue > .9f && red < .05f && green < .05f,
                2 => red > .9f && green > .9f && blue > .9f,
                _ => red > .025f && red < .08f && Math.Abs(red - green) < .01f && Math.Abs(red - blue) < .01f,
            };
            if (!valid) throw new InvalidOperationException($"GPU EXIF {orientation} 角点 {corner} 方向错误：{red}/{green}/{blue}");
        }
    }
    /// <summary>对拍分析输入与 GPU 的显示朝向，防止镜像转置使 CV/DINO 坐标与主图相反。</summary>
    private static void VerifyAnalysisOrientation(MetalPhotoView photo, DecodedImage source)
    {
        using var analysis = ColorEngine.ToAnalysisSrgb8(source);
        using var gpu = photo.CaptureLinearFrame(navigation: true);
        using var rendered = ColorEngine.ToSrgb8(gpu);
        foreach (var point in new[] { (.05, .05), (.95, .05), (.05, .95), (.95, .95) })
        {
            int sourceOffset = (int)(analysis.Height * point.Item2) * analysis.Stride + (int)(analysis.Width * point.Item1) * 4;
            int targetOffset = (int)(rendered.Height * point.Item2) * rendered.Stride + (int)(rendered.Width * point.Item1) * 4;
            for (int channel = 0; channel < 3; channel++)
                if (Math.Abs(analysis.Pixels[sourceOffset + channel] - rendered.Pixels[targetOffset + channel]) > 20)
                    throw new InvalidOperationException($"分析输入与 GPU 朝向不同：EXIF {source.Orientation}");
        }
    }
    /// <summary>轮询真实状态并设定有限超时，失败不输出虚假的通过报告。</summary>
    private static async Task WaitAsync(Func<bool> condition, string name)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(name);
            await Task.Delay(15);
        }
    }
}

/// <summary>可静态分析的原生验收记录，避免剪裁模式下反射序列化。</summary>
public sealed class NativeCheck
{
    public string Check { get; set; } = "";
    public string? File { get; set; }
    public string? Encoding { get; set; }
    public string? ColorSpace { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public double PhysicalScale { get; set; }
    public double Milliseconds { get; set; }
    public bool Proxy { get; set; }
    public string? Mode { get; set; }
    public int Categories { get; set; }
    public bool JsonRoundTrip { get; set; }
    public bool NoAvaloniaMainBitmap { get; set; }
    public long GpuBytes { get; set; }
}

/// <summary>原生验收记录的 source-generated JSON 契约。</summary>
[JsonSerializable(typeof(List<NativeCheck>))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal partial class NativeSmokeJsonContext : JsonSerializerContext;
