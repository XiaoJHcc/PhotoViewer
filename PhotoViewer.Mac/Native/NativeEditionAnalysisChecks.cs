using System.Buffers.Binary;
using PhotoViewer.Apple;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Color;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 双版本共用的分析口径增量回归：CV 诊断页必须先用 <see cref="ColorEngine.ToAnalysisSrgb8"/> 把
/// 真实展示解码产物（macOS 恒 F16 线性 scRGB、宽色域、带待应用方向）规范到显示朝向 sRGB8 再算 CV，
/// 结果与独立参考逐字节一致且不写特征库；AI 版另验 DINO-only 的宽高比取当前展示图显示朝向尺寸、
/// 禁算侧仍显示只读库基线。素材只用自有副本；当前照片停在探针副本上，由调用方收尾。
/// </summary>
internal static class NativeEditionAnalysisChecks
{
    internal static async Task RunAsync(MacMainWindow window, string output, List<string> checks)
    {
        var main = window.Main;
        string suffix = DateTime.UtcNow.Ticks.ToString();
        string p3 = Path.Combine(output, $"edition-cv-p3-{suffix}.jpg");
        string portrait = Path.Combine(output, $"edition-cv-portrait-{suffix}.jpg");
        File.Copy(FixturePaths.Resolve("jpeg", "color-p3.jpg"), p3, overwrite: true);
        EnhancementAcceptance.CreateOrientationJpeg(portrait, 6);
        await VerifyCvDiagnosticAsync(main, "P3 广色域 F16", p3, checks);
        await VerifyCvDiagnosticAsync(main, "EXIF 6 竖拍", portrait, checks);
#if PHOTOVIEWER_DINO
        await VerifyDinoOnlyRatioAsync(output, suffix, checks);
#endif
    }

    /// <summary>
    /// 用真实展示解码器 + 真实 <see cref="CvDiagnosticPresenter"/> 现算，与
    /// "同一解码产物 → ToAnalysisSrgb8 → CvGridExtractor" 的独立参考逐字节对拍。
    /// </summary>
    private static async Task VerifyCvDiagnosticAsync(
        MainViewModel main, string label, string path, List<string> checks)
    {
        await main.FolderVM.OpenImageAsync(new LocalFileRef(path));
        await WaitAsync(() => main.CurrentFile?.Name == Path.GetFileName(path) && main.CurrentFile != null,
            label + " 探针图成为当前照片");
        var file = main.CurrentFile!;
        string? fingerprint = await AnalysisDataReader.ComputeFingerprintAsync(file, CancellationToken.None)
            ?? throw new InvalidOperationException(label + " 探针图无法计算指纹");
        if (await PhotoDatabase.ReadCvGridAsync(fingerprint) != null)
            throw new InvalidOperationException(label + " 探针图已有 CV 入库，无法验证现算不写库");

        using var presenter = new CvDiagnosticPresenter();
        presenter.SetSource(file);
        await WaitAsync(() => presenter.SharpnessTile.Source != null
            || presenter.Status.StartsWith("失败", StringComparison.Ordinal), label + " CV 诊断完成");
        var tile = presenter.SharpnessTile.Source
            ?? throw new InvalidOperationException(label + " CV 诊断未出图：" + presenter.Status);

        using var display = await BitmapLoader.GetBitmapAsync(file.File, CancellationToken.None)
            ?? throw new InvalidOperationException(label + " 展示解码产物为空");
        using var analysis = ColorEngine.ToAnalysisSrgb8(display);
        var grid = await CvGridExtractor.ExtractAsync(analysis);
        using var expected = HeatmapBitmapBuilder.BuildViridis(
            CvHeatmap.BuildSharpness(grid), CvDiagnosticPresenter.CvGridPixels, CvDiagnosticPresenter.CvGridPixels);
        if (!SamePixels(tile, expected))
            throw new InvalidOperationException(
                $"{label} CV 诊断锐度图与 ToAnalysisSrgb8 参考运算不一致（{tile.Width}×{tile.Height} vs {expected.Width}×{expected.Height}）");

        float diagonal = MathF.Sqrt((float)analysis.Width * analysis.Width + (float)analysis.Height * analysis.Height);
        var field = CvHeatmap.BuildShakeField(grid, diagonal);
        if (!SameField(presenter.ShakeTile.ShakeField, field))
            throw new InvalidOperationException(label + " CV 诊断抖动矢量场与参考运算不一致");

        double expectedRatio = (double)analysis.Width / analysis.Height;
        if (Math.Abs(presenter.AspectRatio - expectedRatio) > 0.01)
            throw new InvalidOperationException(
                $"{label} CV 诊断宽高比 {presenter.AspectRatio:0.####} 未取显示朝向尺寸 {expectedRatio:0.####}");
        if (analysis.Orientation != 1)
            throw new InvalidOperationException(label + " 分析图方向未归 1");

        if (await PhotoDatabase.ReadCvGridAsync(fingerprint) != null)
            throw new InvalidOperationException(label + " CV 诊断现算写入了特征库");
        checks.Add(
            $"cv diagnostic {label}: analysis {analysis.Width}×{analysis.Height} sRGB8 matches presenter CV; ratio {expectedRatio:0.###}; no DB write");
    }

#if PHOTOVIEWER_DINO
    /// <summary>
    /// 只开「即时计算 DINO」时：库内 patch + CV 基线直接展示，宽高比必须取当前展示图的显示朝向尺寸，
    /// 不能回落到库内 CV 的容器朝向尺寸。
    /// </summary>
    private static async Task VerifyDinoOnlyRatioAsync(
        string output, string suffix, List<string> checks)
    {
        string path = Path.Combine(output, $"edition-dino-ratio-{suffix}.jpg");
        File.Copy(FixturePaths.Resolve("jpeg", "color-srgb.jpg"), path, overwrite: true);
        var file = new ImageFile(new LocalFileRef(path));
        await file.LoadExifDataAsync();
        await file.LoadBasicPropertiesAsync();
        var input = PhotoFingerprint.BuildInput(file.Name, file.ExifData, file.ModifiedDate?.UtcDateTime);
        if (!input.CaptureTime.HasValue) throw new InvalidOperationException("DINO-only 探针图无法计算指纹");
        string fingerprint = PhotoFingerprint.Compute(input);

        using var baselineImage = CreatePattern(400, 300, invert: true);
        var baselineGrid = await CvGridExtractor.ExtractAsync(baselineImage);
        await PhotoDatabase.WriteIndexedAsync(input, fingerprint, DinoModelResources.ModelId, null,
            CreatePatchBlob(), baselineGrid.Encode(), CvGridResult.CurrentVersion, 400, 300);
        AnalysisResultCache.Invalidate(fingerprint);
        try
        {
            using var display = CreatePattern(240, 160, invert: false, orientation: 6);
            var host = new FakeHost
            {
                CurrentFile = file,
                DisplayImage = display,
                EnhancedDisplay = false,
                ImmediateCompute = true,
                ImmediateCvCompute = false,
            };
            using var presenter = new AnalysisPresenter(host);
            presenter.Refresh();
            await WaitAsync(() => presenter.PcaTile.Source != null, "DINO-only 分析栏贴出库内 patch 基线");
            await WaitAsync(() => presenter.SharpnessTile.Source != null, "DINO-only 禁算侧 CV 基线出图");

            const double expectedRatio = 160d / 240d;
            if (Math.Abs(presenter.AspectRatio - expectedRatio) > 1e-6)
                throw new InvalidOperationException(
                    $"DINO-only 宽高比 {presenter.AspectRatio:0.####} 未取当前展示图显示朝向尺寸 {expectedRatio:0.####}");
            if (presenter.SharpnessTile.Source is not { } tile)
                throw new InvalidOperationException("DINO-only 禁算侧 CV 基线被清空");
            using var expected = HeatmapBitmapBuilder.BuildViridis(
                CvHeatmap.BuildSharpness(baselineGrid), 32, 32);
            if (!SamePixels(tile, expected))
                throw new InvalidOperationException("DINO-only 禁算侧 CV 基线与库内容不一致");
            checks.Add(
                $"dino-only ratio: display {display.Width}×{display.Height} orientation {display.Orientation} → ratio {expectedRatio:0.###}; library CV baseline still shown");
        }
        finally
        {
            AnalysisResultCache.Invalidate(fingerprint);
        }
    }
#endif

    private static async Task WaitAsync(Func<bool> condition, string label)
    {
        for (int attempt = 0; attempt < 800; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("等待超时：" + label);
    }

    private static bool SamePixels(DecodedImage a, DecodedImage b)
        => a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    private static bool SameField(ShakeField? actual, ShakeField expected)
    {
        if (actual == null) return false;
        return SameFloats(actual.Direction, expected.Direction)
            && SameFloats(actual.Width, expected.Width)
            && actual.Mask.AsSpan().SequenceEqual(expected.Mask)
            && SameFloats(actual.LocalConsistency, expected.LocalConsistency)
            && SameFloats(actual.Contrast, expected.Contrast)
            && BitConverter.SingleToInt32Bits(actual.Diagonal) == BitConverter.SingleToInt32Bits(expected.Diagonal);
    }

    private static bool SameFloats(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
        return true;
    }

    private static DecodedImage CreatePattern(int width, int height, bool invert, int orientation = 1)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value = ((x / 6 + y / 6) % 2 == 0 ? 48 : 208) + (x * 37 + y * 11) % 48;
                if (invert) value = 255 - value;
                byte v = (byte)Math.Clamp(value, 0, 255);
                int offset = y * stride + x * 4;
                pixels[offset] = v;
                pixels[offset + 1] = (byte)Math.Clamp(v / 2 + 40, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(255 - v, 0, 255);
                pixels[offset + 3] = 255;
            }
        }
        return new DecodedImage(pixels, width, height, stride, PixelEncoding.Bgra32,
            ImageColorSpace.Srgb, orientation: orientation);
    }

#if PHOTOVIEWER_DINO
    private static byte[] CreatePatchBlob()
    {
        int total = DinoModelResources.PatchTokenCount * DinoModelResources.FeatureDim;
        var blob = new byte[total * sizeof(float)];
        for (int i = 0; i < total; i++)
        {
            float value = MathF.Sin(i * 0.013f) * 0.5f + MathF.Cos((i % 97) * 0.07f) * 0.25f;
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float)), value);
        }
        return blob;
    }
#endif

    private sealed class FakeHost : IAnalysisHost
    {
        public bool AnalysisVisible { get; set; } = true;
        public bool ImmediateCompute { get; set; }
        public bool ImmediateCvCompute { get; set; }
        public ImageFile? CurrentFile { get; set; }
        public DecodedImage? DisplayImage { get; set; }
        public bool EnhancedDisplay { get; set; }
        public bool ThumbnailsBusy => false;
        public event Action? HostChanged;

        public void Raise() => HostChanged?.Invoke();
    }
}
