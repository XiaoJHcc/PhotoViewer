using System.Diagnostics;
using PhotoViewer.Windows.Core.Heif;

namespace HeifFastDecodeTest;

/// <summary>
/// 计时:只测转换器(ffmpeg 解码在计时外),并给出参考管线的分段耗时。
/// </summary>
internal static class Bench
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: bench <file...>");
            return 2;
        }

        var files = new List<string>();
        foreach (var path in args)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*.HIF"));
            else if (File.Exists(path)) files.Add(path);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no input files");
            return 2;
        }

        // 预热(线程池/JIT/静态表)
        Warmup(files[0]);

        var cases = new Case[]
        {
            new("F16 full", true, 0),
            new("BGRA full", false, 0),
            new("F16 @5120", true, 5120),
            new("F16 @4532", true, 4532),
            new("BGRA @840", false, 840),
        };

        Console.WriteLine($"{"case",-14}{"target",-16}{"median ms",12}{"min ms",10}{"MPix/s",10}");
        foreach (var item in cases)
        {
            var durations = new List<double>();
            var scratch = 0L;
            var target = (0, 0);
            foreach (var file in files.Take(7))
            {
                var data = File.ReadAllBytes(file);
                var container = HeifContainer.TryParse(data, out _);
                if (container is null) continue;
                var tiles = DecodeTiles(container, Path.GetFileNameWithoutExtension(file));
                if (tiles is null) continue;
                try
                {
                    var source = BuildSource(container, tiles);
                    var width = item.TargetWidth == 0 ? container.Width : item.TargetWidth;
                    var height = item.TargetWidth == 0
                        ? container.Height
                        : (int)Math.Round((double)container.Height * width / container.Width);
                    target = (width, height);
                    var buffer = new byte[width * height * (item.LinearHalf ? 8 : 4)];
                    var stride = width * (item.LinearHalf ? 8 : 4);

                    for (var repetition = 0; repetition < 3; repetition++)
                    {
                        var watch = Stopwatch.StartNew();
                        var ok = item.LinearHalf
                            ? YuvImageConverter.TryConvertToRgbaLinearF16(source, width, height, buffer, stride, out _)
                            : YuvImageConverter.TryConvertToBgra32(source, width, height, buffer, stride, out _);
                        watch.Stop();
                        if (!ok) throw new InvalidOperationException("conversion rejected");
                        if (repetition < 2) continue;
                        durations.Add(watch.Elapsed.TotalMilliseconds);
                        scratch = YuvImageConverter.LastScratchPeakBytes;
                    }
                }
                finally
                {
                    foreach (var tile in tiles) tile.Dispose();
                }
            }

            if (durations.Count == 0) continue;
            durations.Sort();
            var median = durations[durations.Count / 2];
            var megapixels = target.Item1 * (double)target.Item2 / 1e6;
            Console.WriteLine(
                $"{item.Name,-14}{target.Item1 + "x" + target.Item2,-16}{median,12:F1}{durations[0],10:F1}"
                + $"{megapixels / (median / 1000.0),10:F0}   scratch {scratch / 1048576.0:F1} MB");
        }

        Console.WriteLine();
        MeasureEndToEnd(files);

        Console.WriteLine("reference pipeline stages (libheif 1.15.1 + SkiaSharp), median ms:");
        MeasureReference(files.Take(5).ToList());

        var benchTemp = Path.Combine(Path.GetTempPath(), "HeifFastDecodeTest-bench");
        if (Directory.Exists(benchTemp)) Directory.Delete(benchTemp, true);
        return 0;
    }

    /// <summary>端到端:并行解码瓦片(pvhevc)+ 转换(计时含解码)。</summary>
    private static void MeasureEndToEnd(List<string> files)
    {
        if (!PvHevc.IsAvailable)
        {
            Console.WriteLine();
            Console.WriteLine($"pvhevc.dll not found - end-to-end timings skipped (ffmpeg decode is a stand-in).");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"end-to-end pvhevc v{PvHevc.Version()} parallel decode + convert, median ms:");
        var cases = new (string Label, bool LinearHalf, int TargetWidth)[]
        {
            ("F16 full", true, 0),
            ("F16 @5120", true, 5120),
            ("BGRA @840", false, 840),
        };

        foreach (var (label, linearHalf, targetWidth) in cases)
        {
            var durations = new List<double>();
            var decodeOnly = new List<double>();
            foreach (var file in files.Take(5))
            {
                var data = File.ReadAllBytes(file);
                var container = HeifContainer.TryParse(data, out _);
                if (container is null) continue;

                var decodeWatch = Stopwatch.StartNew();
                var tiles = TileDecodeHarness.DecodeAll(container, string.Empty, true, TileDecodeHarness.Backend.PvHevc);
                decodeWatch.Stop();
                if (tiles is null) continue;
                try
                {
                    var source = BuildSource(container, tiles);
                    var width = targetWidth == 0 ? container.Width : targetWidth;
                    var height = targetWidth == 0
                        ? container.Height
                        : (int)Math.Round((double)container.Height * width / container.Width);
                    var buffer = new byte[width * height * (linearHalf ? 8 : 4)];
                    var stride = width * (linearHalf ? 8 : 4);

                    var watch = Stopwatch.StartNew();
                    var ok = linearHalf
                        ? YuvImageConverter.TryConvertToRgbaLinearF16(source, width, height, buffer, stride, out _)
                        : YuvImageConverter.TryConvertToBgra32(source, width, height, buffer, stride, out _);
                    watch.Stop();
                    if (!ok) continue;
                    durations.Add(decodeWatch.Elapsed.TotalMilliseconds + watch.Elapsed.TotalMilliseconds);
                    decodeOnly.Add(decodeWatch.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    foreach (var tile in tiles) tile.Dispose();
                }
            }

            if (durations.Count == 0) continue;
            durations.Sort();
            decodeOnly.Sort();
            Console.WriteLine(
                $"{label,-14}{"",-16}{durations[durations.Count / 2],12:F1}"
                + $"   (decode {decodeOnly[decodeOnly.Count / 2]:F1} ms)");
        }
    }

    private static void MeasureReference(List<string> files)
    {
        var decode = new List<double>();
        var toBgra = new List<double>();
        var downscale = new List<double>();
        var toF16 = new List<double>();

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var reference = Reference.DecodeRgb24(data, true);
            if (reference is null) continue;
            var width = reference.Width;
            var height = reference.Height;

            var watch = Stopwatch.StartNew();
            var again = Reference.DecodeRgb24(data, true);
            watch.Stop();
            if (again is null) continue;
            decode.Add(watch.Elapsed.TotalMilliseconds);

            watch.Restart();
            var bgra = Reference.RgbToBgra(reference);
            watch.Stop();
            toBgra.Add(watch.Elapsed.TotalMilliseconds);

            var targetWidth = Math.Min(5120, width);
            var targetHeight = (int)Math.Round((double)height * targetWidth / width);
            watch.Restart();
            Reference.DownscaleBgra(bgra, width, height, targetWidth, targetHeight);
            watch.Stop();
            downscale.Add(watch.Elapsed.TotalMilliseconds);

            watch.Restart();
            Reference.ToLinearF16(bgra, width, height);
            watch.Stop();
            toF16.Add(watch.Elapsed.TotalMilliseconds);
        }

        Print("libheif decode -> RGB24", decode);
        Print("RGB24 -> BGRA32", toBgra);
        Print("Skia Mitchell -> 5120", downscale);
        Print("Skia BGRA -> RgbaF16 (full)", toF16);
        return;

        static void Print(string label, List<double> values)
        {
            if (values.Count == 0)
            {
                Console.WriteLine($"{label,-30}{"n/a",12}");
                return;
            }
            values.Sort();
            Console.WriteLine($"{label,-30}{values[values.Count / 2],12:F1}");
        }
    }

    private static void Warmup(string file)
    {
        var data = File.ReadAllBytes(file);
        var container = HeifContainer.TryParse(data, out _);
        if (container is null) return;
        var tiles = DecodeTiles(container, "warmup");
        if (tiles is null) return;
        try
        {
            var source = BuildSource(container, tiles);
            var width = 512;
            var height = (int)Math.Round((double)container.Height * width / container.Width);
            var buffer = new byte[width * height * 8];
            YuvImageConverter.TryConvertToRgbaLinearF16(source, width, height, buffer, width * 8, out _);
            YuvImageConverter.TryConvertToBgra32(source, container.Width, container.Height, new byte[container.Width * container.Height * 4], container.Width * 4, out _);
        }
        finally
        {
            foreach (var tile in tiles) tile.Dispose();
        }
    }

    private static DecodedTile[]? DecodeTiles(HeifContainer container, string name)
        => TileDecodeHarness.DecodeAll(
            container,
            Path.Combine(Path.GetTempPath(), "HeifFastDecodeTest-bench", name),
            true,
            TileDecodeHarness.Preferred);

    private static YuvImage BuildSource(HeifContainer container, DecodedTile[] tiles)
    {
        var yuvTiles = new YuvTile[tiles.Length];
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = container.Tiles[i];
            yuvTiles[i] = tiles[i].ToTile(tile.PixelX, tile.PixelY, tile.Width, tile.Height);
        }
        return new YuvImage
        {
            Tiles = yuvTiles,
            CanvasWidth = container.Width,
            CanvasHeight = container.Height,
            BitDepth = tiles[0].BitDepth,
            Chroma = tiles[0].Chroma,
            MatrixCode = container.Color.HasNclx ? container.Color.Matrix : 5,
            PrimariesCode = container.Color.HasNclx ? container.Color.Primaries : 1,
            TransferCode = container.Color.HasNclx ? container.Color.Transfer : 13,
            FullRange = !container.Color.HasNclx || container.Color.FullRange,
        };
    }

    private readonly record struct Case(string Name, bool LinearHalf, int TargetWidth);
}
