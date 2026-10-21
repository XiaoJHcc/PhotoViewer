using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PhotoViewer.Windows.Core.Heif;

namespace HeifFastDecodeTest;

/// <summary>数值验收:自有转换 vs libheif/Skia 生产管线。</summary>
internal static class Validation
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: validate <outdir> <file...>");
            return 2;
        }

        var outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        var files = ResolveFiles(args[1..]);
        var tempRoot = Path.Combine(Path.GetTempPath(), "HeifFastDecodeTest");
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);

        var report = new List<object>();
        foreach (var file in files)
        {
            ValidateFile(file, outDir, tempRoot, report);
            Console.WriteLine();
        }

        File.WriteAllText(
            Path.Combine(outDir, "validation.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        return 0;
    }

    private static string ValidateFile(string file, string outDir, string tempRoot, List<object> report)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var builder = new StringBuilder(name);
        Console.WriteLine(name);
        builder.AppendLine();

        var data = File.ReadAllBytes(file);
        var container = HeifContainer.TryParse(data, out var reason);
        if (container is null)
        {
            return builder.Append("  ! parse failed: ").Append(reason).ToString();
        }

        var reference = Reference.DecodeRgb24(data, true);
        if (reference is null) return builder.Append("  ! libheif decode failed").ToString();

        var referenceBgra = Reference.RgbToBgra(reference);
        var refWidth = reference.Width;
        var refHeight = reference.Height;
        if (refWidth != container.Width || refHeight != container.Height)
        {
            // IgnoreTransformations 未生效:参考图已按 irot/imir 摆正,先转回存储方向。
            var inverse = Invert(container.ExifOrientation);
            referenceBgra = Rotate(referenceBgra, refWidth, refHeight, inverse, out var rotatedWidth, out var rotatedHeight);
            builder.AppendLine($"  ref libheif returned {refWidth}x{refHeight}, un-rotated with EXIF {inverse} -> {rotatedWidth}x{rotatedHeight}");
            refWidth = rotatedWidth;
            refHeight = rotatedHeight;
        }

        if (refWidth != container.Width || refHeight != container.Height)
        {
            return builder.Append($"  ! dimension mismatch: ours {container.Width}x{container.Height}, ref {refWidth}x{refHeight}").ToString();
        }

        var tileTemp = Path.Combine(tempRoot, name);
        var tiles = TileDecodeHarness.DecodeAll(container, tileTemp, true, TileDecodeHarness.Preferred);
        if (tiles is null) return builder.Append("  ! tile decode failed").ToString();

        try
        {
            var tilesText = $"  tiles: {TileDecodeHarness.Describe(tiles)}";
            Console.WriteLine(tilesText);
            builder.AppendLine(tilesText);
            var yuvTiles = new YuvTile[tiles.Length];
            for (var i = 0; i < tiles.Length; i++)
            {
                var tile = container.Tiles[i];
                yuvTiles[i] = tiles[i].ToTile(tile.PixelX, tile.PixelY, tile.Width, tile.Height);
            }

            var bitDepth = tiles[0].BitDepth;
            var chroma = tiles[0].Chroma;
            var source = new YuvImage
            {
                Tiles = yuvTiles,
                CanvasWidth = container.Width,
                CanvasHeight = container.Height,
                BitDepth = bitDepth,
                Chroma = chroma,
                MatrixCode = container.Color.HasNclx ? container.Color.Matrix : 5,
                PrimariesCode = container.Color.HasNclx ? container.Color.Primaries : 1,
                TransferCode = container.Color.HasNclx ? container.Color.Transfer : 13,
                FullRange = !container.Color.HasNclx || container.Color.FullRange,
            };

            // ---- 全分辨率 BGRA ----
            var fullBgra = new byte[container.Width * container.Height * 4];
            if (!YuvImageConverter.TryConvertToBgra32(source, container.Width, container.Height, fullBgra, container.Width * 4, out var unsupported))
            {
                return builder.Append("  ! converter rejected: ").Append(unsupported).ToString();
            }

            var fullStats = Metrics.CompareBgra(fullBgra, referenceBgra, container.Width, container.Height);
            Console.WriteLine(
                $"  full BGRA vs libheif RGB24: mean {fullStats.Mean:F3} max {fullStats.Max} "
                + $"(B {fullStats.MeanB:F3} G {fullStats.MeanG:F3} R {fullStats.MeanR:F3})");

            // ---- 全分辨率 F16 ----
            var fullF16 = new byte[container.Width * container.Height * 8];
            if (!YuvImageConverter.TryConvertToRgbaLinearF16(source, container.Width, container.Height, fullF16, container.Width * 8, out var f16Reason))
            {
                return builder.Append("  ! F16 converter rejected: ").Append(f16Reason).ToString();
            }

            var referenceF16 = Reference.ToLinearF16(referenceBgra, container.Width, container.Height);
            var mineSrgb = Reference.F16ToSrgb8(fullF16, container.Width, container.Height);
            var referenceSrgb = Reference.F16ToSrgb8(referenceF16, container.Width, container.Height);
            var f16Stats = Metrics.CompareBgra(mineSrgb, referenceSrgb, container.Width, container.Height);
            Console.WriteLine(
                $"  full F16 (both -> sRGB8): mean {f16Stats.Mean:F3} max {f16Stats.Max} "
                + $"(B {f16Stats.MeanB:F3} G {f16Stats.MeanG:F3} R {f16Stats.MeanR:F3})");

            var scales = new (string Label, int Width, int Height)[]
            {
                ("5120", 5120, Scale(container.Width, container.Height, 5120)),
                ("4532", 4532, Scale(container.Width, container.Height, 4532)),
            };

            var scaleResults = new List<object>();
            foreach (var (label, targetWidth, targetHeight) in scales)
            {
                var mine = new byte[targetWidth * targetHeight * 4];
                if (!YuvImageConverter.TryConvertToBgra32(source, targetWidth, targetHeight, mine, targetWidth * 4, out var scaleReason))
                {
                    builder.AppendLine("  ! downscale rejected: " + scaleReason);
                    continue;
                }

                var referenceScaled = Reference.DownscaleBgra(referenceBgra, container.Width, container.Height, targetWidth, targetHeight);
                var stats = Metrics.Statistics(mine, referenceScaled, targetWidth, targetHeight);
                // 隔离重采样器差异:把 Skia Mitchell 作用在"我们自己的全分辨率结果"上再比一次。
                var isolatedScaled = Reference.DownscaleBgra(fullBgra, container.Width, container.Height, targetWidth, targetHeight);
                var isolated = Metrics.Statistics(mine, isolatedScaled, targetWidth, targetHeight);
                Console.WriteLine(
                    $"  down {targetWidth}x{targetHeight} BGRA vs Skia Mitchell: mean {stats.Mean:F3} "
                    + $"p99.9 {stats.Percentile999} max {stats.Max}"
                    + $" | resampler-only: mean {isolated.Mean:F3} p99.9 {isolated.Percentile999} max {isolated.Max}");

                var cropSize = 1024;
                var cropX = Math.Max(0, (targetWidth / 2) - (cropSize / 2));
                var cropY = Math.Max(0, (targetHeight / 2) - (cropSize / 2));
                cropSize = Math.Min(cropSize, Math.Min(targetWidth, targetHeight));
                var minePath = Path.Combine(outDir, $"{name}-{label}-mine.png");
                var referencePath = Path.Combine(outDir, $"{name}-{label}-reference.png");
                Reference.SavePng(minePath, mine, targetWidth, targetHeight, cropX, cropY, cropSize, cropSize);
                Reference.SavePng(referencePath, referenceScaled, targetWidth, targetHeight, cropX, cropY, cropSize, cropSize);
                Console.WriteLine($"    crops: {minePath} | {referencePath}");

                scaleResults.Add(new
                {
                    label,
                    width = targetWidth,
                    height = targetHeight,
                    mean = Math.Round(stats.Mean, 4),
                    p999 = stats.Percentile999,
                    max = stats.Max,
                    resamplerOnlyMean = Math.Round(isolated.Mean, 4),
                    resamplerOnlyP999 = isolated.Percentile999,
                    resamplerOnlyMax = isolated.Max,
                });
            }

            report.Add(new
            {
                file = Path.GetFileName(file),
                width = container.Width,
                height = container.Height,
                tiles = tiles.Length,
                bitDepth,
                chroma = chroma.ToString(),
                fullBgraMean = Math.Round(fullStats.Mean, 4),
                fullBgraMax = fullStats.Max,
                fullF16Mean = Math.Round(f16Stats.Mean, 4),
                fullF16Max = f16Stats.Max,
                scales = scaleResults,
            });
        }
        finally
        {
            foreach (var tile in tiles) tile.Dispose();
        }

        return builder.ToString().TrimEnd();
    }

    private static int Scale(int width, int height, int targetWidth)
        => (int)Math.Round(height * (double)targetWidth / width);

    private static int Invert(int exif) => exif switch
    {
        6 => 8,
        8 => 6,
        _ => exif,
    };

    /// <summary>按 EXIF 方向把像素重新摆放(用于把 libheif 已摆正的参考图转回存储方向)。</summary>
    private static byte[] Rotate(byte[] source, int width, int height, int exif, out int targetWidth, out int targetHeight)
    {
        var swap = exif is 5 or 6 or 7 or 8;
        targetWidth = swap ? height : width;
        targetHeight = swap ? width : height;
        var output = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (nx, ny) = exif switch
                {
                    1 => (x, y),
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (height - 1 - y, x),
                    7 => (height - 1 - y, width - 1 - x),
                    _ => (y, width - 1 - x),
                };
                var sourceIndex = ((y * width) + x) * 4;
                var targetIndex = ((ny * targetWidth) + nx) * 4;
                output[targetIndex] = source[sourceIndex];
                output[targetIndex + 1] = source[sourceIndex + 1];
                output[targetIndex + 2] = source[sourceIndex + 2];
                output[targetIndex + 3] = 255;
            }
        }
        return output;
    }

    private static List<string> ResolveFiles(string[] paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                foreach (var candidate in Directory.EnumerateFiles(full))
                {
                    var extension = Path.GetExtension(candidate);
                    if (extension.Equals(".hif", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(candidate);
                    }
                }
            }
            else if (File.Exists(full))
            {
                result.Add(full);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
